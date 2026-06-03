// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Immutable;
using NetPdf.Css.Parser;
using NetPdf.Css.Properties;
using NetPdf.Layout.Layouters;
using NetPdf.Rendering;
using Xunit;

namespace NetPdf.UnitTests.Rendering;

/// <summary>
/// Unit tests for <see cref="MarginBoxStyle"/> — Phase 3 Task 21 cycle 4, building a margin box's
/// <c>ComputedStyle</c> from its declared longhands + reading the alignment a declared
/// <c>text-align</c> / <c>vertical-align</c> implies.
/// </summary>
public sealed class MarginBoxStyleTests
{
    private static CssDeclaration Decl(string property, string value) =>
        new(property, new CssValue(value), IsImportant: false, CssSourceLocation.Unknown);

    [Fact]
    public void Build_resolves_declared_font_size_and_color()
    {
        var style = MarginBoxStyle.Build(ImmutableArray.Create(
            Decl("font-size", "24px"), Decl("color", "#ff0000")));

        Assert.Equal(24, style.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16), 3);
        Assert.True(style.IsSet(PropertyId.Color));   // a color was resolved onto the style
    }

    [Fact]
    public void Build_leaves_undeclared_properties_unset()
    {
        // No declarations → nothing is set, so the painter/shaper readers fall back to defaults
        // and HorizontalAlignFactor returns null (keep the name-derived alignment).
        var style = MarginBoxStyle.Build(ImmutableArray<CssDeclaration>.Empty);

        Assert.False(style.IsSet(PropertyId.FontSize));
        Assert.False(style.IsSet(PropertyId.TextAlign));
        Assert.Null(MarginBoxStyle.HorizontalAlignFactor(style));
    }

    [Theory]
    [InlineData("center", 0.5)]
    [InlineData("right", 1.0)]
    [InlineData("end", 1.0)]
    [InlineData("justify-all", 1.0)]
    [InlineData("left", 0.0)]
    [InlineData("start", 0.0)]
    [InlineData("justify", 0.0)]
    public void HorizontalAlignFactor_maps_declared_text_align(string value, double expected)
    {
        var style = MarginBoxStyle.Build(ImmutableArray.Create(Decl("text-align", value)));
        Assert.Equal(expected, MarginBoxStyle.HorizontalAlignFactor(style));
    }

    [Theory]
    [InlineData("top", 0.0)]
    [InlineData("middle", 0.5)]
    [InlineData("bottom", 1.0)]
    public void VerticalAlignFactor_maps_declared_vertical_align(string value, double expected)
    {
        Assert.Equal(expected, MarginBoxStyle.VerticalAlignFactor(ImmutableArray.Create(Decl("vertical-align", value))));
    }

    [Fact]
    public void VerticalAlignFactor_is_null_when_absent_or_unrecognized()
    {
        Assert.Null(MarginBoxStyle.VerticalAlignFactor(ImmutableArray.Create(Decl("color", "red"))));
        Assert.Null(MarginBoxStyle.VerticalAlignFactor(ImmutableArray.Create(Decl("vertical-align", "baseline"))));
    }

    [Fact]
    public void VerticalAlignFactor_last_recognized_value_wins()
    {
        var decls = ImmutableArray.Create(Decl("vertical-align", "top"), Decl("vertical-align", "bottom"));
        Assert.Equal(1.0, MarginBoxStyle.VerticalAlignFactor(decls));
    }
}
