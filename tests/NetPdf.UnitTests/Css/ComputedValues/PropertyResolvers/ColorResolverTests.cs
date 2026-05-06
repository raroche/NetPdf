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
/// Unit tests for <see cref="ColorResolver"/> — covers named colors, every hex form,
/// rgb/rgba modern + legacy, hsl/hsla modern + legacy, transparent, and currentcolor.
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
        var slot = ColorResolver.Resolve(value, PropertyId.Color, "color", null, default);
        Assert.Equal(ComputedSlotTag.Color, slot.Tag);
        return slot.AsColor();
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
    public void CurrentColor_packs_to_sentinel(string value) =>
        Assert.Equal(ColorResolver.CurrentColorSentinel, ResolveOk(value));

    // ============================================================
    // Named colors
    // ============================================================

    [Theory]
    [InlineData("red", 0xFFFF0000u)]
    [InlineData("Red", 0xFFFF0000u)]
    [InlineData("REBECCAPURPLE", 0xFF663399u)]
    [InlineData("aqua", 0xFF00FFFFu)]
    [InlineData("cyan", 0xFF00FFFFu)]            // alias of aqua
    [InlineData("grey", 0xFF808080u)]
    [InlineData("gray", 0xFF808080u)]            // alias of grey
    public void Named_colors_resolve(string value, uint expected) =>
        Assert.Equal(expected, ResolveOk(value));

    // ============================================================
    // Hex
    // ============================================================

    [Theory]
    [InlineData("#f00", 0xFFFF0000u)]            // #rgb
    [InlineData("#0f0", 0xFF00FF00u)]
    [InlineData("#abc", 0xFFAABBCCu)]
    [InlineData("#f00f", 0xFFFF0000u)]           // #rgba (full alpha)
    [InlineData("#f008", 0x88FF0000u)]           // #rgba half alpha
    [InlineData("#FF0000", 0xFFFF0000u)]         // #rrggbb
    [InlineData("#abcdef", 0xFFABCDEFu)]
    [InlineData("#ff0000ff", 0xFFFF0000u)]       // #rrggbbaa opaque
    [InlineData("#ff000080", 0x80FF0000u)]       // #rrggbbaa 50% alpha
    public void Hex_forms_resolve(string value, uint expected) =>
        Assert.Equal(expected, ResolveOk(value));

    [Theory]
    [InlineData("#")]                            // empty
    [InlineData("#fg0")]                          // bad hex digit
    [InlineData("#1234")]                         // valid 4-digit form
    [InlineData("#12345")]                        // 5 chars — invalid
    [InlineData("#1234567")]                      // 7 chars — invalid
    public void Hex_malformed_emits_diagnostic_when_invalid(string value)
    {
        var sink = new CapturingSink();
        var slot = ColorResolver.Resolve(value, PropertyId.Color, "color", sink, default);
        if (value == "#1234")
        {
            // 4-digit form is valid (#rgba) — exclude this from the bad-shape assertion.
            Assert.Equal(ComputedSlotTag.Color, slot.Tag);
            Assert.Empty(sink.Diagnostics);
        }
        else
        {
            Assert.Equal(ComputedSlot.Unset, slot);
            Assert.Single(sink.Diagnostics);
        }
    }

    // ============================================================
    // rgb() / rgba()
    // ============================================================

    [Theory]
    [InlineData("rgb(255, 0, 0)", 0xFFFF0000u)]
    [InlineData("rgb(255 0 0)", 0xFFFF0000u)]               // modern
    [InlineData("rgba(255, 0, 0, 0.5)", 0x80FF0000u)]
    [InlineData("rgba(255, 0, 0, 50%)", 0x80FF0000u)]
    [InlineData("rgb(255 0 0 / 0.5)", 0x80FF0000u)]         // modern with slash
    [InlineData("rgb(255 0 0 / 50%)", 0x80FF0000u)]
    [InlineData("rgb(100%, 0%, 0%)", 0xFFFF0000u)]
    [InlineData("rgb(0 0 0)", 0xFF000000u)]
    [InlineData("rgba(0,0,0,0)", 0x00000000u)]
    public void Rgb_forms_resolve(string value, uint expected) =>
        Assert.Equal(expected, ResolveOk(value));

    [Theory]
    [InlineData("rgb()")]
    [InlineData("rgb(255)")]
    [InlineData("rgb(255, 0, 0, 0.5, 0.5)")] // 5 components
    [InlineData("rgb(foo, 0, 0)")]
    public void Rgb_malformed_emits_diagnostic(string value)
    {
        var sink = new CapturingSink();
        var slot = ColorResolver.Resolve(value, PropertyId.Color, "color", sink, default);
        Assert.Equal(ComputedSlot.Unset, slot);
        Assert.Single(sink.Diagnostics);
    }

    [Fact]
    public void Rgb_clamps_overflow_channels()
    {
        // 300 should clamp to 255; -10 should clamp to 0.
        Assert.Equal(0xFFFF0000u, ResolveOk("rgb(300, -10, 0)"));
    }

    // ============================================================
    // hsl() / hsla()
    // ============================================================

    [Theory]
    [InlineData("hsl(0, 100%, 50%)", 0xFFFF0000u)]    // pure red
    [InlineData("hsl(120, 100%, 50%)", 0xFF00FF00u)]  // pure green
    [InlineData("hsl(240, 100%, 50%)", 0xFF0000FFu)]  // pure blue
    [InlineData("hsl(0, 0%, 0%)", 0xFF000000u)]       // black
    [InlineData("hsl(0, 0%, 100%)", 0xFFFFFFFFu)]     // white
    [InlineData("hsl(0 100% 50%)", 0xFFFF0000u)]      // modern syntax
    [InlineData("hsla(0, 100%, 50%, 0.5)", 0x80FF0000u)]
    [InlineData("hsl(0 100% 50% / 0.5)", 0x80FF0000u)]
    public void Hsl_forms_resolve(string value, uint expected) =>
        Assert.Equal(expected, ResolveOk(value));

    [Theory]
    [InlineData("hsl(360, 100%, 50%)", 0xFFFF0000u)]  // 360 wraps to 0
    [InlineData("hsl(-60, 100%, 50%)", 0xFFFF00FFu)]  // negative wraps
    [InlineData("hsl(0.5turn, 100%, 50%)", 0xFF00FFFFu)] // 180deg = cyan
    [InlineData("hsl(3.14159265358979rad, 100%, 50%)", 0xFF00FFFFu)] // ≈180deg = cyan
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
    public void Modern_color_functions_emit_diagnostic(string value)
    {
        var sink = new CapturingSink();
        var slot = ColorResolver.Resolve(value, PropertyId.Color, "color", sink, default);
        Assert.Equal(ComputedSlot.Unset, slot);
        Assert.Single(sink.Diagnostics);
        Assert.Equal(CssDiagnosticCodes.CssPropertyValueInvalid001, sink.Diagnostics[0].Code);
    }

    // ============================================================
    // Garbage
    // ============================================================

    [Theory]
    [InlineData("not-a-color")]
    [InlineData("16px")]
    [InlineData("")]
    public void Garbage_emits_diagnostic(string value)
    {
        var sink = new CapturingSink();
        var slot = ColorResolver.Resolve(value, PropertyId.Color, "color", sink, default);
        Assert.Equal(ComputedSlot.Unset, slot);
        if (value.Length > 0)
            Assert.Single(sink.Diagnostics);
    }
}
