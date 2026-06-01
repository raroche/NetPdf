// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Diagnostics;
using NetPdf.Css.Properties;
using Xunit;

namespace NetPdf.UnitTests.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Phase 5 layout→PDF cycle 4 — unit tests for <see cref="FontSizeResolver"/>
/// (CSS Fonts 4 §3.4). Absolute forms (keywords + lengths) resolve in the dispatch;
/// parent-relative forms defer + resolve via <see cref="FontSizeResolver.TryResolveRelativeToParent"/>.
/// </summary>
public sealed class FontSizeResolverTests
{
    private static ResolverResult Resolve(string value) =>
        FontSizeResolver.Resolve(value, PropertyId.FontSize, "font-size", null, default);

    [Theory]
    [InlineData("medium", 16.0)]
    [InlineData("large", 16.0 * 6.0 / 5.0)]      // 19.2
    [InlineData("x-large", 24.0)]
    [InlineData("xx-large", 32.0)]
    [InlineData("small", 16.0 * 8.0 / 9.0)]      // ~14.22
    [InlineData("x-small", 12.0)]
    [InlineData("MEDIUM", 16.0)]                 // case-insensitive
    public void Absolute_size_keywords_resolve_to_px(string value, double expectedPx)
    {
        var result = Resolve(value);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.LengthPx, result.Slot.Tag);
        Assert.Equal(expectedPx, result.Slot.AsLengthPx(), 3);
    }

    [Theory]
    [InlineData("14px", 14.0)]
    [InlineData("12pt", 16.0)]   // 12 * 96/72
    [InlineData("1in", 96.0)]
    public void Absolute_lengths_resolve_to_px(string value, double expectedPx)
    {
        var result = Resolve(value);
        Assert.True(result.IsResolved);
        Assert.Equal(expectedPx, result.Slot.AsLengthPx(), 3);
    }

    [Theory]
    [InlineData("2em")]
    [InlineData("150%")]
    [InlineData("larger")]
    [InlineData("smaller")]
    [InlineData("1.5rem")]   // root-relative — deferred (follow-up)
    public void Parent_or_root_relative_forms_defer(string value)
    {
        var result = Resolve(value);
        Assert.True(result.IsDeferred);
        Assert.Equal(value, result.RawText);
    }

    [Fact]
    public void Negative_length_is_invalid()
    {
        Assert.True(Resolve("-5px").IsInvalid);
    }

    [Theory]
    [InlineData("-50%")]
    [InlineData("-1em")]
    [InlineData("-0.5ex")]
    [InlineData("-2ch")]
    public void Negative_relative_sizes_are_invalid(string value)
    {
        // Post-PR-#120 review (P2): a negative parent-relative font-size is invalid at
        // computed-value time — it must NOT defer + silently snap to the 16px default.
        Assert.True(Resolve(value).IsInvalid);
    }

    [Fact]
    public void Invalid_relative_size_emits_a_diagnostic()
    {
        var sink = new CapturingSink();
        var result = FontSizeResolver.Resolve("-50%", PropertyId.FontSize, "font-size", sink, default);

        Assert.True(result.IsInvalid);
        Assert.Contains(sink.Diagnostics, d => d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001);
    }

    [Fact]
    public void Rem_is_not_misrouted_as_an_em_relative_size()
    {
        // Post-PR-#120 review (P2): `rem` ends in "em" but is root-relative, not
        // parent-relative — it must DEFER (a follow-up), never invalidate.
        var result = Resolve("2rem");
        Assert.True(result.IsDeferred);
        Assert.Equal("2rem", result.RawText);
    }

    [Theory]
    [InlineData("2em", 20.0, 40.0)]
    [InlineData("150%", 20.0, 30.0)]
    [InlineData("larger", 20.0, 24.0)]      // × 1.2
    [InlineData("smaller", 24.0, 20.0)]     // ÷ 1.2
    public void TryResolveRelativeToParent_computes_against_parent(string raw, double parent, double expected)
    {
        Assert.True(FontSizeResolver.TryResolveRelativeToParent(raw, parent, out var px));
        Assert.Equal(expected, px, 3);
    }

    [Fact]
    public void TryResolveRelativeToParent_returns_false_for_root_relative_units()
    {
        // rem is root-relative, not parent-relative — left deferred.
        Assert.False(FontSizeResolver.TryResolveRelativeToParent("1rem", 20.0, out _));
    }

    [Fact]
    public void Dispatch_routes_font_size()
    {
        var resolved = PropertyResolverDispatch.Resolve(PropertyId.FontSize, "medium");
        Assert.True(resolved.IsResolved);
        Assert.Equal(16.0, resolved.Slot.AsLengthPx(), 3);

        Assert.True(PropertyResolverDispatch.Resolve(PropertyId.FontSize, "1.5em").IsDeferred);
    }

    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }
}
