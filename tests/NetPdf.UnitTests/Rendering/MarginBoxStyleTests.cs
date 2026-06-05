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
        // margin is NOT in the whitelist (a margin box's own margin doesn't apply), so it's ignored.
        // Supported props — color, the border-* longhands (border cycle), and now padding-* (padding
        // cycle) — resolve.
        var style = MarginBoxStyle.Build(ImmutableArray.Create(
            Decl("margin-left", "50px"), Decl("padding-left", "50px"),
            Decl("border-top-width", "5px"), Decl("color", "red")));
        Assert.False(style.IsSet(PropertyId.MarginLeft));     // margin still ignored
        Assert.True(style.IsSet(PropertyId.PaddingLeft));     // padding now materialized
        Assert.True(style.IsSet(PropertyId.BorderTopWidth));  // border materialized
        Assert.True(style.IsSet(PropertyId.Color));
    }

    [Fact]
    public void Build_materializes_explicit_width_and_height()
    {
        // width/height join the cascade whitelist (explicit-size cycle) → resolved to LengthPx slots
        // the painter reads to size the box along its §5.3 variable axis.
        var style = MarginBoxStyle.Build(ImmutableArray.Create(
            Decl("width", "120px"), Decl("height", "40px")));
        Assert.Equal(120, style.ReadLengthPxOrDefault(PropertyId.Width, defaultPx: -1), 3);
        Assert.Equal(40, style.ReadLengthPxOrDefault(PropertyId.Height, defaultPx: -1), 3);
    }

    [Fact]
    public void Build_does_not_inherit_width_or_height()
    {
        // width/height are NOT CSS inherited properties — a parent (page-context) value must not flow
        // down to the child margin box (mirrors background-color/border/padding).
        var parent = MarginBoxStyle.Build(ImmutableArray.Create(Decl("width", "200px")));
        var child = MarginBoxStyle.Build(ImmutableArray<CssDeclaration>.Empty, parent);
        Assert.False(child.IsSet(PropertyId.Width));
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
    [InlineData("4ex", 32.0)]       // ex ≈ 0.5em → 16 × 4 × 0.5 (CSS Values §6.1.2 approximation)
    [InlineData("4ch", 32.0)]       // ch ≈ 0.5em → 16 × 4 × 0.5
    public void Build_resolves_relative_size_keywords_and_percent(string value, double expectedPx)
    {
        var style = MarginBoxStyle.Build(ImmutableArray.Create(Decl("font-size", value)));
        Assert.Equal(expectedPx, style.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16), 3);
    }

    [Fact]
    public void Build_resolves_bolder_font_weight_against_the_default_when_no_parent()
    {
        // No parent → bolder resolves against the initial normal (400) → 700 (CSS Fonts 4 §2.2.1).
        // (Resolution against a NON-default parent is covered by the theory below.)
        var style = MarginBoxStyle.Build(ImmutableArray.Create(Decl("font-weight", "bolder")));
        Assert.Equal(700, style.ReadFontWeight());
    }

    [Theory]
    // Pin the CSS Fonts 4 §2.2.1 relative-weight TABLE is exercised through the margin-box deferred
    // path against NON-default parents (the resolver table itself is pinned in FontWeightResolverTests;
    // this proves DeferredFontResolver threads the parent's weight correctly).
    [InlineData("bolder", "300", 400)]   // < 350 → 400
    [InlineData("bolder", "600", 900)]   // ≥ 550 → 900
    [InlineData("lighter", "700", 400)]  // [550,750) → 400
    [InlineData("lighter", "900", 700)]  // ≥ 750 → 700
    public void Build_resolves_relative_font_weight_against_a_non_default_parent(
        string relative, string parentWeight, int expected)
    {
        var parent = MarginBoxStyle.Build(ImmutableArray.Create(Decl("font-weight", parentWeight)));
        var child = MarginBoxStyle.Build(ImmutableArray.Create(Decl("font-weight", relative)), parent);
        Assert.Equal(expected, child.ReadFontWeight());
    }

    [Fact]
    public void Build_leaves_rem_font_size_deferred_and_unresolved()
    {
        // PIN: rem isn't parent-relative (needs the root font-size threaded) → it stays DEFERRED (the
        // raw "2rem" is preserved, not unset) and the reader falls back to the default (16px).
        var style = MarginBoxStyle.Build(ImmutableArray.Create(Decl("font-size", "2rem")));
        Assert.True(style.IsDeferred(PropertyId.FontSize));   // distinguishes "deferred" from "unset"
        Assert.Equal(16, style.ReadLengthPxOrDefault(PropertyId.FontSize, defaultPx: 16), 3);
    }

    // ---- background-color (cycle 8) ----

    [Fact]
    public void Build_resolves_declared_background_color()
    {
        var style = MarginBoxStyle.Build(ImmutableArray.Create(Decl("background-color", "#00ff00")));
        Assert.True(style.IsSet(PropertyId.BackgroundColor));
        Assert.True(FragmentPainter.TryResolveColor(style.Get(PropertyId.BackgroundColor), 0xFF000000u, out var argb));
        Assert.Equal(0xFF00FF00u, argb);
    }

    [Fact]
    public void Build_does_not_inherit_background_color()
    {
        // background-color is NOT a CSS inherited property — a box that declares none does not pick
        // up the parent/page-context background (cycle 8; only the font-*/color whitelist inherits).
        var parent = MarginBoxStyle.Build(ImmutableArray.Create(Decl("background-color", "#ff0000")));
        var child = MarginBoxStyle.Build(ImmutableArray<CssDeclaration>.Empty, parent);
        Assert.False(child.IsSet(PropertyId.BackgroundColor));
    }

    [Fact]
    public void Build_background_color_important_beats_a_later_normal()
    {
        // background-color goes through the same per-property cascade (importance then source order).
        var style = MarginBoxStyle.Build(ImmutableArray.Create(
            Decl("background-color", "#ff0000", important: true), Decl("background-color", "#0000ff")));
        Assert.True(FragmentPainter.TryResolveColor(style.Get(PropertyId.BackgroundColor), 0xFF000000u, out var argb));
        Assert.Equal(0xFFFF0000u, argb);   // red !important wins
    }

    [Fact]
    public void Build_background_color_inherit_copies_the_parent()
    {
        // background-color is normally non-inherited, but an explicit `inherit` copies the parent
        // (post-PR-#137 review P2 — property-aware CSS-wide handling).
        var parent = MarginBoxStyle.Build(ImmutableArray.Create(Decl("background-color", "#ff0000")));
        var child = MarginBoxStyle.Build(ImmutableArray.Create(Decl("background-color", "inherit")), parent);
        Assert.True(FragmentPainter.TryResolveColor(child.Get(PropertyId.BackgroundColor), 0xFF000000u, out var argb));
        Assert.Equal(0xFFFF0000u, argb);
    }

    [Fact]
    public void Build_background_color_unset_is_initial_for_the_non_inherited_property()
    {
        // `unset` = initial for a NON-inherited property → transparent (cleared), NOT the parent.
        var parent = MarginBoxStyle.Build(ImmutableArray.Create(Decl("background-color", "#ff0000")));
        var child = MarginBoxStyle.Build(ImmutableArray.Create(Decl("background-color", "unset")), parent);
        Assert.False(child.IsSet(PropertyId.BackgroundColor));
    }

    [Fact]
    public void Build_color_unset_inherits_for_the_inherited_property()
    {
        // REGRESSION (the property-aware refactor): `unset` on an INHERITED property (color) still
        // keeps the parent's value (= inherit), unlike the non-inherited background-color above.
        var parent = MarginBoxStyle.Build(ImmutableArray.Create(Decl("color", "#ff0000")));
        var child = MarginBoxStyle.Build(ImmutableArray.Create(Decl("color", "unset")), parent);
        Assert.True(FragmentPainter.TryResolveColor(child.Get(PropertyId.Color), 0xFF000000u, out var argb));
        Assert.Equal(0xFFFF0000u, argb);
    }

    // ---- border (border cycle) ----

    [Fact]
    public void Build_resolves_declared_border_longhands()
    {
        var style = MarginBoxStyle.Build(ImmutableArray.Create(
            Decl("border-top-style", "solid"), Decl("border-top-width", "2px"), Decl("border-top-color", "#ff0000")));
        Assert.True(style.IsSet(PropertyId.BorderTopStyle));
        Assert.True(style.IsSet(PropertyId.BorderTopWidth));
        Assert.True(style.IsSet(PropertyId.BorderTopColor));
    }

    [Fact]
    public void Build_does_not_inherit_border()
    {
        // border-* are NOT CSS inherited properties — a box that declares none doesn't pick up the
        // parent's border (only the font-*/color whitelist inherits).
        var parent = MarginBoxStyle.Build(ImmutableArray.Create(Decl("border-top-style", "solid")));
        var child = MarginBoxStyle.Build(ImmutableArray<CssDeclaration>.Empty, parent);
        Assert.False(child.IsSet(PropertyId.BorderTopStyle));
    }

    // ---- padding (padding cycle) ----

    [Fact]
    public void Build_resolves_declared_padding_longhands()
    {
        var style = MarginBoxStyle.Build(ImmutableArray.Create(
            Decl("padding-top", "10px"), Decl("padding-left", "20px")));
        Assert.True(style.IsSet(PropertyId.PaddingTop));
        Assert.True(style.IsSet(PropertyId.PaddingLeft));
    }

    [Fact]
    public void Build_does_not_inherit_padding()
    {
        // padding-* are NOT CSS inherited properties — a box that declares none doesn't pick up the
        // parent's padding.
        var parent = MarginBoxStyle.Build(ImmutableArray.Create(Decl("padding-top", "10px")));
        var child = MarginBoxStyle.Build(ImmutableArray<CssDeclaration>.Empty, parent);
        Assert.False(child.IsSet(PropertyId.PaddingTop));
    }

    [Fact]
    public void Build_surfaces_a_rejected_padding_shorthand()
    {
        // A `padding` shorthand reaching Build is one CssParserAdapter could NOT expand (a valid one
        // becomes padding-* longhands upstream); Build surfaces it instead of silently dropping.
        var sink = new CapturingSink();
        var style = MarginBoxStyle.Build(
            ImmutableArray.Create(Decl("padding", "10xyz")), parentStyle: null, diagnostics: sink);
        Assert.Contains(sink.Diagnostics, d => d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001);
        Assert.False(style.IsSet(PropertyId.PaddingTop));
    }

    [Theory]
    [InlineData("padding-left", "10%")]    // a percentage (resolves against the containing block)
    [InlineData("padding-top", "25%")]
    [InlineData("padding-left", "1em")]     // a font-relative length (left deferred by the resolver)
    [InlineData("padding-right", "1rem")]
    [InlineData("padding-bottom", "5vw")]   // a viewport-relative length
    public void Build_diagnoses_and_drops_non_absolute_padding(string property, string value)
    {
        // A percentage or a font-/viewport-relative padding is a VALID value (the cascade accepts it)
        // but the margin-box painter can't resolve it to used px yet — it reads padding via
        // ReadLengthPxOrZero, which honors only a LengthPx slot. So it must be diagnosed + DROPPED
        // (unset → 0), not left as a slot the painter silently renders as 0 (review P2 + Copilot).
        var sink = new CapturingSink();
        var style = MarginBoxStyle.Build(
            ImmutableArray.Create(Decl(property, value)), parentStyle: null, diagnostics: sink);
        Assert.Contains(sink.Diagnostics, d => d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001);
        Assert.False(style.IsSet(PropertyMetadata.NameToId[property]));   // dropped, not a non-px slot
    }

    [Theory]
    [InlineData("10px")]
    [InlineData("0")]      // the unitless zero is a valid absolute length
    public void Build_keeps_absolute_padding_without_a_diagnostic(string value)
    {
        // The non-absolute guard must NOT touch an absolute-length padding (no false positives) — and
        // must NOT double-diagnose (the value resolves to a LengthPx slot, so it's applied as-is).
        var sink = new CapturingSink();
        var style = MarginBoxStyle.Build(
            ImmutableArray.Create(Decl("padding-left", value)), parentStyle: null, diagnostics: sink);
        Assert.Empty(sink.Diagnostics);
        Assert.True(style.IsSet(PropertyId.PaddingLeft));
    }

    [Theory]
    [InlineData("width", "10em")]    // font-relative (the resolver defers it — no resolver diagnostic)
    [InlineData("height", "5vh")]    // viewport-relative
    [InlineData("width", "2rem")]
    [InlineData("height", "5vw")]
    public void Build_diagnoses_and_drops_a_deferred_explicit_size(string property, string value)
    {
        // A font-/viewport-relative width/height is a VALID value the resolver DEFERS, but the painter
        // only honors an absolute length or a percentage (TryReadExplicitSizePx); a deferred size would
        // silently fall back to shrink-to-fit. It must be diagnosed + DROPPED (unset), not left as a
        // deferred slot the painter silently ignores (review P2).
        var sink = new CapturingSink();
        var style = MarginBoxStyle.Build(
            ImmutableArray.Create(Decl(property, value)), parentStyle: null, diagnostics: sink);
        Assert.Contains(sink.Diagnostics, d => d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001);
        Assert.False(style.IsSet(PropertyMetadata.NameToId[property]));   // dropped, not a deferred slot
    }

    [Theory]
    [InlineData("100px")]   // absolute length → LengthPx, honored as-is
    [InlineData("50%")]     // percentage → resolved against the band, honored
    [InlineData("auto")]    // the default — intended shrink-to-fit, NOT a warning
    public void Build_keeps_a_supported_explicit_size_without_a_diagnostic(string value)
    {
        // No false positives: an absolute length, a percentage, and `auto` must NOT trip the deferred-
        // size guard (`auto` is the intended shrink-to-fit, not an unsupported value).
        var sink = new CapturingSink();
        MarginBoxStyle.Build(ImmutableArray.Create(Decl("width", value)), parentStyle: null, diagnostics: sink);
        Assert.Empty(sink.Diagnostics);
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

    // ---- rejected `border` shorthand marker (review P2) ----

    [Theory]
    [InlineData("border", "1bananas solid red")]      // a malformed width → un-expandable
    [InlineData("border-top", "1px solid red blue")]  // too many components → un-expandable
    public void Build_surfaces_a_rejected_border_shorthand_and_applies_no_border(string property, string value)
    {
        // A `border` / `border-<side>` declaration reaching Build is one CssParserAdapter could NOT
        // expand (it keeps the raw shorthand as a marker — a valid one becomes border-* longhands).
        // Build surfaces it via the diagnostics sink instead of silently dropping, and materializes
        // NONE of it (no border longhand is set).
        var sink = new CapturingSink();
        var style = MarginBoxStyle.Build(
            ImmutableArray.Create(Decl(property, value)), parentStyle: null, diagnostics: sink);

        Assert.Contains(sink.Diagnostics, d => d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001);
        Assert.False(style.IsSet(PropertyId.BorderTopStyle));
        Assert.False(style.IsSet(PropertyId.BorderTopWidth));
        Assert.False(style.IsSet(PropertyId.BorderTopColor));
    }

    [Fact]
    public void Build_does_not_diagnose_expanded_border_longhands()
    {
        // The expanded longhands (what CssParserAdapter actually emits for a VALID `border`) resolve
        // cleanly with NO invalid-value diagnostic — only an un-expandable raw shorthand is a marker.
        var sink = new CapturingSink();
        MarginBoxStyle.Build(ImmutableArray.Create(
            Decl("border-top-style", "solid"), Decl("border-top-width", "2px"), Decl("border-top-color", "red")),
            parentStyle: null, diagnostics: sink);
        Assert.Empty(sink.Diagnostics);
    }

    [Theory]
    [InlineData("border-width", "1bananas")]   // a bad width unit → un-expandable
    [InlineData("border-style", "solid wavy")] // an unsupported style keyword
    [InlineData("border-color", "red notacolor")]
    public void Build_surfaces_a_rejected_border_box_shorthand(string property, string value)
    {
        // A `border-width`/`-style`/`-color` box shorthand reaching Build is one CssParserAdapter could
        // NOT expand (a valid one becomes the four per-edge longhands upstream); surface it instead of
        // silently dropping.
        var sink = new CapturingSink();
        var style = MarginBoxStyle.Build(
            ImmutableArray.Create(Decl(property, value)), parentStyle: null, diagnostics: sink);
        Assert.Contains(sink.Diagnostics, d => d.Code == CssDiagnosticCodes.CssPropertyValueInvalid001);
        Assert.False(style.IsSet(PropertyId.BorderTopWidth));
        Assert.False(style.IsSet(PropertyId.BorderTopStyle));
    }

    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }
}
