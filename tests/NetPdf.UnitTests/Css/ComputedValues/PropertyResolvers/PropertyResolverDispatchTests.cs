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
/// Integration tests for <see cref="PropertyResolverDispatch"/> against the cycle-1-review
/// <see cref="ResolverResult"/> contract. Per-resolver behavior is exhaustively covered
/// by the leaf resolver tests; these tests focus on the dispatch wiring + the
/// Resolved/Deferred/Invalid state distinction.
/// </summary>
public sealed class PropertyResolverDispatchTests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    [Fact]
    public void Color_property_routes_to_color_resolver()
    {
        var result = PropertyResolverDispatch.Resolve(PropertyId.Color, "red");
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Color, result.Slot.Tag);
        Assert.Equal(0xFFFF0000u, result.Slot.AsColor());
    }

    [Fact]
    public void BackgroundColor_with_hex_routes_to_color_resolver()
    {
        var result = PropertyResolverDispatch.Resolve(PropertyId.BackgroundColor, "#abc");
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Color, result.Slot.Tag);
        Assert.Equal(0xFFAABBCCu, result.Slot.AsColor());
    }

    [Fact]
    public void Width_auto_routes_to_length_resolver_keyword_branch()
    {
        var result = PropertyResolverDispatch.Resolve(PropertyId.Width, "auto");
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Keyword, result.Slot.Tag);
        Assert.Equal(LengthResolver.KeywordIdAuto, result.Slot.AsKeyword());
    }

    [Fact]
    public void PaddingTop_with_percentage_routes_to_length_resolver()
    {
        var result = PropertyResolverDispatch.Resolve(PropertyId.PaddingTop, "50%");
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Percentage, result.Slot.Tag);
        Assert.Equal(50.0, result.Slot.AsPercentage(), 2);
    }

    [Fact]
    public void FlexGrow_routes_to_number_resolver()
    {
        var result = PropertyResolverDispatch.Resolve(PropertyId.FlexGrow, "0.5");
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Number, result.Slot.Tag);
        Assert.Equal(0.5, result.Slot.AsNumber(), 5);
    }

    [Fact]
    public void Display_routes_to_keyword_resolver()
    {
        var result = PropertyResolverDispatch.Resolve(PropertyId.Display, "flex");
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Keyword, result.Slot.Tag);
    }

    [Fact]
    public void Empty_value_is_invalid()
    {
        var result = PropertyResolverDispatch.Resolve(PropertyId.Color, "");
        Assert.True(result.IsInvalid);
    }

    [Fact]
    public void Whitespace_only_value_is_invalid()
    {
        var result = PropertyResolverDispatch.Resolve(PropertyId.Color, "   \t\n");
        Assert.True(result.IsInvalid);
    }

    [Fact]
    public void Unsupported_PropertyType_defers_with_raw_text()
    {
        // Per Rec 1: cycle-2 PropertyTypes (FontFamilyList here) defer instead of
        // returning Invalid. The raw text rides along for the cycle-2 resolver to
        // pick up.
        var sink = new CapturingSink();
        var result = PropertyResolverDispatch.Resolve(PropertyId.FontFamily, "Arial, sans-serif", sink);
        Assert.True(result.IsDeferred);
        Assert.Equal("Arial, sans-serif", result.RawText);
        Assert.Empty(sink.Diagnostics);
    }

    [Fact]
    public void Width_em_value_defers_via_length_resolver()
    {
        // Per Rec 1: deferred values (font/viewport/container-relative units)
        // surface as ResolverResult.Deferred — distinct from Invalid.
        var sink = new CapturingSink();
        var result = PropertyResolverDispatch.Resolve(PropertyId.Width, "2em", sink);
        Assert.True(result.IsDeferred);
        Assert.Equal("2em", result.RawText);
        Assert.Empty(sink.Diagnostics);
    }

    [Fact]
    public void Diagnostic_propagates_through_dispatch()
    {
        var sink = new CapturingSink();
        var result = PropertyResolverDispatch.Resolve(PropertyId.Color, "not-a-color", sink);
        Assert.True(result.IsInvalid);
        Assert.Single(sink.Diagnostics);
        Assert.Equal(CssDiagnosticCodes.CssPropertyValueInvalid001, sink.Diagnostics[0].Code);
    }

    [Fact]
    public void Color_default_canvastext_resolves_via_dispatch_after_rec_2()
    {
        // Regression for Rec 2: the cascade-time default for `color` is `canvastext`.
        // Cycle 1 emitted a diagnostic for this; Rec 2 made it resolve through the
        // system-color table.
        var sink = new CapturingSink();
        var result = PropertyResolverDispatch.Resolve(PropertyId.Color, "canvastext", sink);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Color, result.Slot.Tag);
        Assert.Empty(sink.Diagnostics);
    }
}
