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
/// Integration tests for <see cref="PropertyResolverDispatch"/> — verifies that each
/// PropertyType routes to the right resolver. Per-resolver behavior is exhaustively
/// covered by the leaf resolver tests; these tests focus on the dispatch wiring.
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
        var slot = PropertyResolverDispatch.Resolve(PropertyId.Color, "red");
        Assert.Equal(ComputedSlotTag.Color, slot.Tag);
        Assert.Equal(0xFFFF0000u, slot.AsColor());
    }

    [Fact]
    public void BackgroundColor_with_hex_routes_to_color_resolver()
    {
        var slot = PropertyResolverDispatch.Resolve(PropertyId.BackgroundColor, "#abc");
        Assert.Equal(ComputedSlotTag.Color, slot.Tag);
        Assert.Equal(0xFFAABBCCu, slot.AsColor());
    }

    [Fact]
    public void BorderTopWidth_with_px_routes_to_length_resolver()
    {
        var slot = PropertyResolverDispatch.Resolve(PropertyId.BorderTopWidth, "16px");
        // BorderTopWidth is type LineWidth — cycle 1 doesn't handle that yet, so it
        // returns Unset. (LineWidth is in the deferred set.)
        Assert.Equal(ComputedSlot.Unset, slot);
    }

    [Fact]
    public void Width_auto_routes_to_length_resolver_keyword_branch()
    {
        var slot = PropertyResolverDispatch.Resolve(PropertyId.Width, "auto");
        Assert.Equal(ComputedSlotTag.Keyword, slot.Tag);
        Assert.Equal(LengthResolver.KeywordIdAuto, slot.AsKeyword());
    }

    [Fact]
    public void PaddingTop_with_percentage_routes_to_length_resolver()
    {
        var slot = PropertyResolverDispatch.Resolve(PropertyId.PaddingTop, "50%");
        Assert.Equal(ComputedSlotTag.Percentage, slot.Tag);
        Assert.Equal(50.0, slot.AsPercentage(), 2);
    }

    [Fact]
    public void FlexGrow_routes_to_number_resolver()
    {
        var slot = PropertyResolverDispatch.Resolve(PropertyId.FlexGrow, "0.5");
        Assert.Equal(ComputedSlotTag.Number, slot.Tag);
        Assert.Equal(0.5, slot.AsNumber(), 5);
    }

    [Fact]
    public void Display_routes_to_keyword_resolver()
    {
        var slot = PropertyResolverDispatch.Resolve(PropertyId.Display, "flex");
        Assert.Equal(ComputedSlotTag.Keyword, slot.Tag);
    }

    [Fact]
    public void Empty_value_returns_unset()
    {
        var slot = PropertyResolverDispatch.Resolve(PropertyId.Color, "");
        Assert.Equal(ComputedSlot.Unset, slot);
    }

    [Fact]
    public void Whitespace_only_value_returns_unset()
    {
        var slot = PropertyResolverDispatch.Resolve(PropertyId.Color, "   \t\n");
        Assert.Equal(ComputedSlot.Unset, slot);
    }

    [Fact]
    public void Unsupported_PropertyType_returns_unset_no_diagnostic()
    {
        // FontFamily is type FontFamilyList — cycle 1 doesn't wire it. No diagnostic.
        var sink = new CapturingSink();
        var slot = PropertyResolverDispatch.Resolve(PropertyId.FontFamily, "Arial, sans-serif", sink);
        Assert.Equal(ComputedSlot.Unset, slot);
        Assert.Empty(sink.Diagnostics);
    }

    [Fact]
    public void Diagnostic_propagates_through_dispatch()
    {
        var sink = new CapturingSink();
        var slot = PropertyResolverDispatch.Resolve(PropertyId.Color, "not-a-color", sink);
        Assert.Equal(ComputedSlot.Unset, slot);
        Assert.Single(sink.Diagnostics);
        Assert.Equal(CssDiagnosticCodes.CssPropertyValueInvalid001, sink.Diagnostics[0].Code);
    }
}
