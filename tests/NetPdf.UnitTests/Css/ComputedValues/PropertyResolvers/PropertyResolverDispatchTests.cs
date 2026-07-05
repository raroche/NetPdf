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
    public void Unsupported_PropertyType_returns_UnsupportedUnvalidated_with_raw_text()
    {
        // Per the hardening review: still-unwired cycle-2 PropertyTypes (Content
        // here) surface as UnsupportedUnvalidated — distinct from Deferred which
        // means "validated but needs context". The raw text rides along for the
        // future resolver to pick up. (line-height resolves as of the line-height cycle.)
        var sink = new CapturingSink();
        var result = PropertyResolverDispatch.Resolve(PropertyId.Content, "open-quote", sink);
        Assert.True(result.IsUnsupportedUnvalidated);
        Assert.False(result.IsDeferred);
        Assert.Equal("open-quote", result.RawText);
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

    // ── CSS-wide keywords (CSS Cascade L5 §7) — centrally handled by the dispatch ──────────
    // These are valid on EVERY property; the dispatch intercepts them before the per-type
    // resolver. `initial` resolves the property's INITIAL value; the others fall through as
    // "declaration ignored" (Invalid → the cascade materializes inherited-or-initial). None
    // of them may emit CSS-PROPERTY-VALUE-INVALID-001 — they are valid, not authoring errors.

    [Fact]
    public void Initial_on_line_height_resolves_to_the_properties_initial_value_not_the_keyword_table()
    {
        // Regression for the #271 P2 review: `line-height: initial` must compute to the
        // property's initial value (`normal` → Keyword(Normal)), NOT be silently dropped as
        // Invalid (which — on an inherited property under a parent `line-height: 3` — would
        // wrongly leave the inherited 3 instead of resetting to normal).
        var sink = new CapturingSink();
        var result = PropertyResolverDispatch.Resolve(PropertyId.LineHeight, "initial", sink);

        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Keyword, result.Slot.Tag);
        Assert.Equal(LineHeightResolver.Normal, result.Slot.AsKeyword());
        // Identical to spelling the initial value out explicitly.
        var normal = PropertyResolverDispatch.Resolve(PropertyId.LineHeight, "normal");
        Assert.Equal(normal.Slot.AsKeyword(), result.Slot.AsKeyword());
        Assert.Empty(sink.Diagnostics);
    }

    [Fact]
    public void Initial_on_a_non_inherited_property_resolves_to_its_default_value()
    {
        // background-attachment's initial value is `scroll`; `initial` must resolve it, not warn.
        var sink = new CapturingSink();
        var result = PropertyResolverDispatch.Resolve(PropertyId.BackgroundAttachment, "initial", sink);
        var scroll = PropertyResolverDispatch.Resolve(PropertyId.BackgroundAttachment, "scroll");

        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Keyword, result.Slot.Tag);
        Assert.Equal(scroll.Slot.AsKeyword(), result.Slot.AsKeyword());
        Assert.Empty(sink.Diagnostics);
    }

    [Theory]
    [InlineData("inherit")]
    [InlineData("unset")]
    [InlineData("revert")]
    [InlineData("revert-layer")]
    public void Non_initial_css_wide_keywords_are_ignored_without_a_warning(string keyword)
    {
        // inherit/unset/revert fall through as Invalid (the cascade materializes them as the
        // inherited value for an inherited property / the initial value otherwise) — but they
        // must NOT emit the "not an admitted keyword" diagnostic that a genuine typo would.
        var sink = new CapturingSink();
        var result = PropertyResolverDispatch.Resolve(PropertyId.LineHeight, keyword, sink);

        Assert.True(result.IsInvalid);
        Assert.Empty(sink.Diagnostics);
    }

    [Fact]
    public void Css_wide_keyword_is_case_insensitive_and_ignores_surrounding_whitespace()
    {
        var sink = new CapturingSink();
        var result = PropertyResolverDispatch.Resolve(PropertyId.LineHeight, "  INITIAL  ", sink);

        Assert.True(result.IsResolved);
        Assert.Equal(LineHeightResolver.Normal, result.Slot.AsKeyword());
        Assert.Empty(sink.Diagnostics);
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
