// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using System.Collections.Generic;
using System.Collections.Immutable;
using NetPdf.Css.ComputedValues;
using NetPdf.Css.Diagnostics;
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
    private static CssDeclaration Decl(string property, string value, bool important = false) =>
        new(property, new CssValue(value), important, CssSourceLocation.Unknown);

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
        Assert.False(style.IsSet(PropertyId.Color));
        Assert.Null(MarginBoxStyle.HorizontalAlignFactor(ImmutableArray<CssDeclaration>.Empty));
    }

    [Theory]
    [InlineData("center", 0.5)]
    [InlineData("right", 1.0)]
    [InlineData("end", 1.0)]
    [InlineData("left", 0.0)]
    [InlineData("start", 0.0)]
    [InlineData("justify", 0.0)]
    [InlineData("justify-all", 0.0)]   // single-line margin box → justify-all behaves as start (review P3)
    public void HorizontalAlignFactor_maps_declared_text_align(string value, double expected)
    {
        Assert.Equal(expected, MarginBoxStyle.HorizontalAlignFactor(ImmutableArray.Create(Decl("text-align", value))));
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

    // ---- !important cascade per style property (review P2) ----

    [Fact]
    public void Build_important_font_size_beats_a_later_normal()
    {
        var style = MarginBoxStyle.Build(ImmutableArray.Create(
            Decl("font-size", "24px", important: true), Decl("font-size", "10px")));
        Assert.Equal(24, style.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16), 3);
    }

    [Fact]
    public void Build_later_important_font_size_wins()
    {
        var style = MarginBoxStyle.Build(ImmutableArray.Create(
            Decl("font-size", "24px", important: true), Decl("font-size", "10px", important: true)));
        Assert.Equal(10, style.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16), 3);
    }

    [Fact]
    public void HorizontalAlignFactor_honors_important_text_align()
    {
        // text-align: right !important must beat a later normal text-align: left.
        var decls = ImmutableArray.Create(
            Decl("text-align", "right", important: true), Decl("text-align", "left"));
        Assert.Equal(1.0, MarginBoxStyle.HorizontalAlignFactor(decls));
    }

    [Fact]
    public void VerticalAlignFactor_honors_important()
    {
        var decls = ImmutableArray.Create(
            Decl("vertical-align", "top", important: true), Decl("vertical-align", "bottom"));
        Assert.Equal(0.0, MarginBoxStyle.VerticalAlignFactor(decls));   // !important top beats later bottom
    }

    // ---- Whitelist: unsupported properties are not materialized (review P2) ----

    [Fact]
    public void Build_ignores_unsupported_properties()
    {
        // padding/border affect the painter's content origin; they must NOT be materialized this
        // cycle, so they can't shift margin-box text. Supported props still resolve.
        var style = MarginBoxStyle.Build(ImmutableArray.Create(
            Decl("padding-left", "50px"), Decl("border-top-width", "5px"), Decl("color", "red")));
        Assert.False(style.IsSet(PropertyId.PaddingLeft));
        Assert.False(style.IsSet(PropertyId.BorderTopWidth));
        Assert.True(style.IsSet(PropertyId.Color));
    }

    // ---- Inheritance from the parent (page context / root), cycle 5 ----

    [Fact]
    public void Build_inherits_a_supported_property_from_the_parent()
    {
        var parent = MarginBoxStyle.Build(ImmutableArray.Create(Decl("color", "#ff0000")));
        var child = MarginBoxStyle.Build(ImmutableArray<CssDeclaration>.Empty, parent);

        Assert.True(child.IsSet(PropertyId.Color));
        Assert.True(FragmentPainter.TryResolveColor(child.Get(PropertyId.Color), 0xFF000000u, out var argb));
        Assert.Equal(0xFFFF0000u, argb);   // inherited red
    }

    [Fact]
    public void Build_own_declaration_overrides_the_inherited_value()
    {
        var parent = MarginBoxStyle.Build(ImmutableArray.Create(Decl("color", "#ff0000")));
        var child = MarginBoxStyle.Build(ImmutableArray.Create(Decl("color", "#0000ff")), parent);

        Assert.True(FragmentPainter.TryResolveColor(child.Get(PropertyId.Color), 0xFF000000u, out var argb));
        Assert.Equal(0xFF0000FFu, argb);   // own blue overrides the inherited red
    }

    [Fact]
    public void Build_inherits_the_font_family_side_table_payload()
    {
        // font-family resolves to a side-table slot; the payload (the family list), not just the
        // index marker, must be copied on inheritance.
        var parent = MarginBoxStyle.Build(ImmutableArray.Create(Decl("font-family", "Courier")));
        var child = MarginBoxStyle.Build(ImmutableArray<CssDeclaration>.Empty, parent);

        Assert.True(child.IsSet(PropertyId.FontFamily));
        Assert.True(child.TryGetSideTablePayloadRaw(PropertyId.FontFamily, out var payload));
        Assert.NotNull(payload);
    }

    [Fact]
    public void Build_does_not_inherit_non_inherited_vertical_align()
    {
        // vertical-align is not an inherited property + isn't materialized — a parent's value
        // doesn't flow to the child via the style (VerticalAlignFactor reads the box's own decls).
        var parent = MarginBoxStyle.Build(ImmutableArray.Create(Decl("vertical-align", "top")));
        var child = MarginBoxStyle.Build(ImmutableArray<CssDeclaration>.Empty, parent);
        Assert.False(child.IsSet(PropertyId.VerticalAlign));
    }

    // ---- CSS-wide keywords, cycle-5 review P2 ----

    [Fact]
    public void Build_css_wide_initial_resets_an_inherited_value()
    {
        // color: initial clears the inherited red → the slot is unset → the reader falls back to
        // the property's initial/default (NOT the inherited value).
        var parent = MarginBoxStyle.Build(ImmutableArray.Create(Decl("color", "#ff0000")));
        var child = MarginBoxStyle.Build(ImmutableArray.Create(Decl("color", "initial")), parent);
        Assert.False(child.IsSet(PropertyId.Color));
    }

    [Fact]
    public void Build_css_wide_inherit_keeps_the_parent_value()
    {
        var parent = MarginBoxStyle.Build(ImmutableArray.Create(Decl("color", "#ff0000")));
        var child = MarginBoxStyle.Build(ImmutableArray.Create(Decl("color", "inherit")), parent);
        Assert.True(FragmentPainter.TryResolveColor(child.Get(PropertyId.Color), 0xFF000000u, out var argb));
        Assert.Equal(0xFFFF0000u, argb);   // inherit keeps the parent's red
    }

    [Fact]
    public void HorizontalAlignFactor_initial_is_start_others_keep_name_derived()
    {
        // text-align: initial → start (0); inherit/unset/revert aren't modeled for alignment → null
        // (keep the name-derived default).
        Assert.Equal(0.0, MarginBoxStyle.HorizontalAlignFactor(ImmutableArray.Create(Decl("text-align", "initial"))));
        Assert.Null(MarginBoxStyle.HorizontalAlignFactor(ImmutableArray.Create(Decl("text-align", "inherit"))));
    }

    // ---- parent-relative font resolution (cycle 7) ----

    [Fact]
    public void Build_resolves_em_font_size_against_the_default_when_no_parent()
    {
        // No parent → the relative em resolves against the initial 16px → 2em = 32px.
        var style = MarginBoxStyle.Build(ImmutableArray.Create(Decl("font-size", "2em")));
        Assert.Equal(32, style.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16), 3);
    }

    [Fact]
    public void Build_resolves_em_font_size_against_the_parent_chain()
    {
        // The box's 1.5em resolves against the parent's resolved 20px → 30px (CSS Page 3 chain).
        var parent = MarginBoxStyle.Build(ImmutableArray.Create(Decl("font-size", "20px")));
        var child = MarginBoxStyle.Build(ImmutableArray.Create(Decl("font-size", "1.5em")), parent);
        Assert.Equal(30, child.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16), 3);
    }

    [Theory]
    [InlineData("larger", 19.2)]    // 16 × 1.2
    [InlineData("smaller", 16.0 / 1.2)]
    [InlineData("150%", 24.0)]      // 16 × 1.5
    public void Build_resolves_relative_size_keywords_and_percent(string value, double expectedPx)
    {
        var style = MarginBoxStyle.Build(ImmutableArray.Create(Decl("font-size", value)));
        Assert.Equal(expectedPx, style.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16), 3);
    }

    [Fact]
    public void Build_resolves_bolder_font_weight_against_the_parent()
    {
        // bolder against the default normal (400) → 700 (CSS Fonts 4 §2.2.1).
        var style = MarginBoxStyle.Build(ImmutableArray.Create(Decl("font-weight", "bolder")));
        Assert.Equal(700, style.ReadFontWeight());
    }

    [Fact]
    public void Build_leaves_rem_font_size_deferred_and_unresolved()
    {
        // PIN: rem isn't parent-relative (needs the root font-size threaded) → it stays deferred and
        // the reader falls back to the default (16px), NOT 2 × something.
        var style = MarginBoxStyle.Build(ImmutableArray.Create(Decl("font-size", "2rem")));
        Assert.Equal(16, style.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16), 3);
    }

    // ---- rejected `font` shorthand marker (review #3) ----

    [Theory]
    [InlineData("caption")]                    // a system-font keyword
    [InlineData("italic 12bananas serif")]     // a malformed/invalid shorthand
    public void Build_surfaces_a_rejected_font_shorthand_and_applies_no_partial_style(string fontValue)
    {
        // A `font` declaration reaching Build is one CssParserAdapter could NOT expand (it keeps the
        // raw declaration as a marker). Build surfaces it via the diagnostics sink instead of
        // silently ignoring it, and applies NONE of it as a style (atomic — no partial survives).
        var sink = new CapturingSink();
        var style = MarginBoxStyle.Build(
            ImmutableArray.Create(Decl("font", fontValue)), parentStyle: null, diagnostics: sink);

        Assert.Contains(sink.Diagnostics, d => d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001);
        Assert.False(style.IsSet(PropertyId.FontSize));
        Assert.False(style.IsSet(PropertyId.FontStyle));
        Assert.False(style.IsSet(PropertyId.FontFamily));
    }

    [Fact]
    public void Build_does_not_diagnose_when_no_font_marker_is_present()
    {
        // A normal supported declaration emits no font-shorthand diagnostic (no false positives).
        var sink = new CapturingSink();
        MarginBoxStyle.Build(ImmutableArray.Create(Decl("color", "#ff0000")), parentStyle: null, diagnostics: sink);
        Assert.Empty(sink.Diagnostics);
    }

    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }
}
