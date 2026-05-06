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
/// Unit tests for <see cref="ColorResolver"/> against the cycle-1-review
/// <see cref="ResolverResult"/> contract — covers named colors, system colors,
/// every hex form, rgb/rgba modern + legacy with strict syntax enforcement,
/// hsl/hsla, transparent, and the dedicated CurrentColor tag.
/// </summary>
public sealed class ColorResolverTests
{
    private sealed class CapturingSink : ICssDiagnosticsSink
    {
        public List<CssDiagnostic> Diagnostics { get; } = new();
        public void Emit(CssDiagnostic d) => Diagnostics.Add(d);
    }

    private static uint ResolveOk(string value)
    {
        var result = ColorResolver.Resolve(value, PropertyId.Color, "color", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Color, result.Slot.Tag);
        return result.Slot.AsColor();
    }

    // ============================================================
    // CSS-wide values
    // ============================================================

    [Theory]
    [InlineData("transparent")]
    [InlineData("TRANSPARENT")]
    [InlineData("Transparent")]
    public void Transparent_packs_to_zero_argb(string value) =>
        Assert.Equal(0x00000000u, ResolveOk(value));

    [Theory]
    [InlineData("currentcolor")]
    [InlineData("CurrentColor")]
    [InlineData("CURRENTCOLOR")]
    public void CurrentColor_uses_dedicated_tag_not_argb_sentinel(string value)
    {
        // Rec 3: dedicated CurrentColor tag, not a packed argb sentinel that could
        // collide with rgba(0, 0, 1, 0).
        var result = ColorResolver.Resolve(value, PropertyId.Color, "color", null, default);
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.CurrentColor, result.Slot.Tag);
        Assert.True(result.Slot.IsCurrentColor);
        Assert.NotEqual(ComputedSlotTag.Color, result.Slot.Tag);
    }

    [Fact]
    public void User_color_that_would_have_collided_with_old_sentinel_is_distinct()
    {
        // Rec 3 regression: the old code packed currentcolor as 0x00000001. A user
        // writing rgba(0, 0, 1, 0) — a transparent very-dark-blue — packed identically
        // and was indistinguishable. With the dedicated tag, the two are distinct.
        var rgbaResult = ColorResolver.Resolve("rgba(0, 0, 1, 0)", PropertyId.Color, "color", null, default);
        var ccResult   = ColorResolver.Resolve("currentcolor", PropertyId.Color, "color", null, default);

        Assert.True(rgbaResult.IsResolved);
        Assert.True(ccResult.IsResolved);
        Assert.Equal(ComputedSlotTag.Color, rgbaResult.Slot.Tag);
        Assert.Equal(ComputedSlotTag.CurrentColor, ccResult.Slot.Tag);
        Assert.NotEqual(rgbaResult.Slot, ccResult.Slot);
    }

    // ============================================================
    // Named colors
    // ============================================================

    [Theory]
    [InlineData("red", 0xFFFF0000u)]
    [InlineData("Red", 0xFFFF0000u)]
    [InlineData("REBECCAPURPLE", 0xFF663399u)]
    [InlineData("aqua", 0xFF00FFFFu)]
    [InlineData("cyan", 0xFF00FFFFu)]
    [InlineData("grey", 0xFF808080u)]
    [InlineData("gray", 0xFF808080u)]
    public void Named_colors_resolve(string value, uint expected) =>
        Assert.Equal(expected, ResolveOk(value));

    // ============================================================
    // Rec 2 — System colors so canvastext / canvas / etc. parse
    // ============================================================

    [Theory]
    [InlineData("canvas",        0xFFFFFFFFu)]   // paper white
    [InlineData("canvastext",    0xFF000000u)]   // ink black
    [InlineData("CanvasText",    0xFF000000u)]   // case-insensitive
    [InlineData("linktext",      0xFF0000EEu)]
    [InlineData("visitedtext",   0xFF551A8Bu)]
    [InlineData("activetext",    0xFFEE0000u)]
    [InlineData("buttontext",    0xFF000000u)]
    [InlineData("graytext",      0xFF808080u)]
    [InlineData("mark",          0xFFFFFF00u)]
    [InlineData("highlighttext", 0xFF000000u)]
    public void System_colors_resolve(string value, uint expected) =>
        Assert.Equal(expected, ResolveOk(value));

    [Fact]
    public void Color_default_canvastext_resolves_via_dispatch()
    {
        // The properties.json default for `color` is "canvastext". Before Rec 2, this
        // would emit a diagnostic for every styled element with no explicit color.
        var result = PropertyResolverDispatch.Resolve(PropertyId.Color, "canvastext");
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.Color, result.Slot.Tag);
        Assert.Equal(0xFF000000u, result.Slot.AsColor());
    }

    // ============================================================
    // Hex
    // ============================================================

    [Theory]
    [InlineData("#f00",       0xFFFF0000u)]
    [InlineData("#abc",       0xFFAABBCCu)]
    [InlineData("#f008",      0x88FF0000u)]
    [InlineData("#FF0000",    0xFFFF0000u)]
    [InlineData("#ff000080",  0x80FF0000u)]
    public void Hex_forms_resolve(string value, uint expected) =>
        Assert.Equal(expected, ResolveOk(value));

    [Theory]
    [InlineData("#")]
    [InlineData("#fg0")]
    [InlineData("#12345")]
    [InlineData("#1234567")]
    public void Hex_malformed_is_invalid(string value)
    {
        var sink = new CapturingSink();
        var result = ColorResolver.Resolve(value, PropertyId.Color, "color", sink, default);
        Assert.True(result.IsInvalid);
        Assert.Single(sink.Diagnostics);
    }

    // ============================================================
    // rgb() / rgba() — strict modern vs legacy (Rec 7)
    // ============================================================

    [Theory]
    [InlineData("rgb(255, 0, 0)",          0xFFFF0000u)]   // legacy 3-arg
    [InlineData("rgb(255 0 0)",            0xFFFF0000u)]   // modern 3-arg
    [InlineData("rgba(255, 0, 0, 0.5)",    0x80FF0000u)]   // legacy 4-arg
    [InlineData("rgba(255, 0, 0, 50%)",    0x80FF0000u)]
    [InlineData("rgb(255 0 0 / 0.5)",      0x80FF0000u)]   // modern with slash
    [InlineData("rgb(255 0 0 / 50%)",      0x80FF0000u)]
    [InlineData("rgb(100%, 0%, 0%)",       0xFFFF0000u)]
    [InlineData("rgba(0,0,0,0)",           0x00000000u)]
    public void Rgb_pure_legacy_or_pure_modern_resolves(string value, uint expected) =>
        Assert.Equal(expected, ResolveOk(value));

    [Theory]
    [InlineData("rgb(255, 0, 0 / 0.5)")]      // commas + slash — explicit mix
    [InlineData("hsl(0, 100%, 50% / 0.5)")]   // same rule for hsl
    public void Comma_plus_slash_is_rejected_with_mixed_message(string value)
    {
        var sink = new CapturingSink();
        var result = ColorResolver.Resolve(value, PropertyId.Color, "color", sink, default);
        Assert.True(result.IsInvalid);
        Assert.Single(sink.Diagnostics);
        Assert.Contains("mixed", sink.Diagnostics[0].Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("rgba(255 0 0, 0.5)")]        // hybrid (whitespace 3-tuple + comma alpha)
    [InlineData("rgb(, , 0)")]                // dangling commas
    public void Other_malformed_legacy_or_modern_arrangements_are_invalid(string value)
    {
        // These don't match the simple "comma + slash" mixed pattern but are still
        // ill-formed — they fall through to the per-channel parse failure path.
        var sink = new CapturingSink();
        var result = ColorResolver.Resolve(value, PropertyId.Color, "color", sink, default);
        Assert.True(result.IsInvalid);
        Assert.NotEmpty(sink.Diagnostics);
    }

    [Theory]
    [InlineData("rgb()")]
    [InlineData("rgb(255)")]
    [InlineData("rgb(255, 0, 0, 0.5, 0.5)")]
    [InlineData("rgb(foo, 0, 0)")]
    public void Rgb_malformed_is_invalid(string value)
    {
        var sink = new CapturingSink();
        var result = ColorResolver.Resolve(value, PropertyId.Color, "color", sink, default);
        Assert.True(result.IsInvalid);
        Assert.Single(sink.Diagnostics);
    }

    [Fact]
    public void Rgb_clamps_overflow_channels() =>
        Assert.Equal(0xFFFF0000u, ResolveOk("rgb(300, -10, 0)"));

    // ============================================================
    // hsl() / hsla()
    // ============================================================

    [Theory]
    [InlineData("hsl(0, 100%, 50%)",        0xFFFF0000u)]
    [InlineData("hsl(120, 100%, 50%)",      0xFF00FF00u)]
    [InlineData("hsl(240, 100%, 50%)",      0xFF0000FFu)]
    [InlineData("hsl(0, 0%, 0%)",           0xFF000000u)]
    [InlineData("hsl(0, 0%, 100%)",         0xFFFFFFFFu)]
    [InlineData("hsl(0 100% 50%)",          0xFFFF0000u)]
    [InlineData("hsla(0, 100%, 50%, 0.5)",  0x80FF0000u)]
    [InlineData("hsl(0 100% 50% / 0.5)",    0x80FF0000u)]
    public void Hsl_forms_resolve(string value, uint expected) =>
        Assert.Equal(expected, ResolveOk(value));

    [Theory]
    [InlineData("hsl(360, 100%, 50%)",                          0xFFFF0000u)]
    [InlineData("hsl(-60, 100%, 50%)",                          0xFFFF00FFu)]
    [InlineData("hsl(0.5turn, 100%, 50%)",                      0xFF00FFFFu)]
    [InlineData("hsl(3.14159265358979rad, 100%, 50%)",          0xFF00FFFFu)]
    public void Hsl_hue_units_normalize(string value, uint expected) =>
        Assert.Equal(expected, ResolveOk(value));

    // ============================================================
    // Modern color spaces deferred with diagnostic
    // ============================================================

    [Theory]
    [InlineData("oklch(0.5 0.1 180)")]
    [InlineData("oklab(0.5 0.1 0.1)")]
    [InlineData("lab(50% 40 0)")]
    [InlineData("lch(50% 40 180)")]
    [InlineData("color(srgb 1 0 0)")]
    [InlineData("color-mix(in srgb, red, blue)")]
    public void Modern_color_functions_are_invalid_until_cycle_2(string value)
    {
        var sink = new CapturingSink();
        var result = ColorResolver.Resolve(value, PropertyId.Color, "color", sink, default);
        Assert.True(result.IsInvalid);
        Assert.Single(sink.Diagnostics);
        Assert.Equal(CssDiagnosticCodes.CssPropertyValueInvalid001, sink.Diagnostics[0].Code);
    }

    // ============================================================
    // Garbage
    // ============================================================

    [Theory]
    [InlineData("not-a-color")]
    [InlineData("16px")]
    public void Garbage_is_invalid(string value)
    {
        var sink = new CapturingSink();
        var result = ColorResolver.Resolve(value, PropertyId.Color, "color", sink, default);
        Assert.True(result.IsInvalid);
        Assert.Single(sink.Diagnostics);
    }
}
