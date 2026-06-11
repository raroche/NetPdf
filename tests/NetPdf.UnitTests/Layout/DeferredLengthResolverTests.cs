// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
using Xunit;

namespace NetPdf.UnitTests.Layout;

/// <summary>
/// Body context-dependent cycle — <see cref="DeferredLengthResolver"/> tests: the post-build
/// in-place pass that rewrites deferred font-/viewport-relative body lengths (units AND math
/// functions) into used-px slots, against the owning box's font-size / the root element's /
/// the page box.
/// </summary>
public sealed class DeferredLengthResolverTests
{
    private const double PageW = 800.0;
    private const double PageH = 600.0;

    private static ComputedStyle MakeStyle(double fontSizePx = 16)
    {
        var s = ComputedStyle.RentForExclusiveTesting();
        s.Set(PropertyId.FontSize, ComputedSlot.FromLengthPx(fontSizePx));
        return s;
    }

    /// <summary>Root → element box (the root element, font-size 32) → child box.</summary>
    private static (Box Root, Box Element, Box Child) MakeTree(
        ComputedStyle elementStyle, ComputedStyle childStyle)
    {
        var root = Box.CreateRoot(MakeStyle());
        var element = Box.Anonymous(BoxKind.AnonymousBlock, elementStyle);
        var child = Box.Anonymous(BoxKind.AnonymousBlock, childStyle);
        element.AppendChild(child);
        root.AppendChild(element);
        return (root, element, child);
    }

    [Fact]
    public void Deferred_relative_length_resolves_in_place()
    {
        // (PropertyId is internal, so the cases live in the body rather than InlineData rows.)
        var cases = new (string Raw, PropertyId Id, double ExpectedPx)[]
        {
            ("2em", PropertyId.Width, 40.0),                  // em → the box's own font-size (20)
            ("50vw", PropertyId.Width, 400.0),                // vw → the page box width
            ("25vh", PropertyId.Height, 150.0),
            ("calc(2em + 10px)", PropertyId.PaddingLeft, 50.0), // math fn with an em term
            ("min(5vw, 100px)", PropertyId.Width, 40.0),        // 5vw = 40 < 100
            ("1.5em", PropertyId.LineHeight, 30.0),
        };
        foreach (var (raw, id, expectedPx) in cases)
        {
            var style = MakeStyle(fontSizePx: 20);
            style.SetDeferred(id, raw);
            var (root, _, _) = MakeTree(style, MakeStyle());

            DeferredLengthResolver.ResolveTreeInPlace(root, PageW, PageH);

            Assert.Equal(expectedPx, style.ReadLengthPxOrZero(id), 3);
        }
    }

    [Fact]
    public void Rem_resolves_against_the_root_element_font_size()
    {
        var elementStyle = MakeStyle(fontSizePx: 32);   // the ROOT ELEMENT box (root's first child)
        var childStyle = MakeStyle(fontSizePx: 20);
        childStyle.SetDeferred(PropertyId.Width, "2rem");
        var (root, _, _) = MakeTree(elementStyle, childStyle);

        DeferredLengthResolver.ResolveTreeInPlace(root, PageW, PageH);

        Assert.Equal(64.0, childStyle.ReadLengthPxOrZero(PropertyId.Width), 3); // 2 × 32, not 2 × 20
    }

    [Fact]
    public void Negative_relative_margin_resolves_but_negative_width_stays_deferred()
    {
        // Margins admit negatives (the property's actual range); width is a non-negative
        // property, so a negative unit value stays deferred (mirrors the cascade's
        // negative-literal reject — no silent clamp on the unit path).
        var style = MakeStyle(fontSizePx: 20);
        style.SetDeferred(PropertyId.MarginLeft, "-2em");
        style.SetDeferred(PropertyId.Width, "-2em");
        var (root, _, _) = MakeTree(style, MakeStyle());

        DeferredLengthResolver.ResolveTreeInPlace(root, PageW, PageH);

        Assert.Equal(-40.0, style.ReadLengthPxOrZero(PropertyId.MarginLeft), 3);
        Assert.True(style.IsDeferred(PropertyId.Width));
    }

    [Fact]
    public void Negative_calc_margin_resolves_and_negative_calc_width_clamps()
    {
        // The calc path follows the §10.5 used-value range clamp per property: a margin keeps
        // the negative result, a width clamps at 0 (matching the cascade's absolute-calc rule).
        var style = MakeStyle(fontSizePx: 20);
        style.SetDeferred(PropertyId.MarginLeft, "calc(0px - 2em)");
        style.SetDeferred(PropertyId.Width, "calc(0px - 2em)");
        var (root, _, _) = MakeTree(style, MakeStyle());

        DeferredLengthResolver.ResolveTreeInPlace(root, PageW, PageH);

        Assert.Equal(-40.0, style.ReadLengthPxOrZero(PropertyId.MarginLeft), 3);
        Assert.Equal(0.0, style.ReadLengthPxOrZero(PropertyId.Width), 3);
        Assert.False(style.IsDeferred(PropertyId.Width));
    }

    [Fact]
    public void Percentage_math_function_stays_deferred()
    {
        // Defense in depth — the cascade classifier rejects % math functions, but if a raw
        // reaches the pass anyway the NaN percent base keeps it deferred (no bogus value).
        var style = MakeStyle();
        style.SetDeferred(PropertyId.Width, "calc(50% + 2em)");
        var (root, _, _) = MakeTree(style, MakeStyle());

        DeferredLengthResolver.ResolveTreeInPlace(root, PageW, PageH);

        Assert.True(style.IsDeferred(PropertyId.Width));
    }

    [Fact]
    public void Trig_math_function_resolves_via_the_pass()
    {
        // The §10.8/§10.9 functions ride the same deferred-math path: hypot over em terms.
        var style = MakeStyle(fontSizePx: 20);
        style.SetDeferred(PropertyId.Width, "hypot(3em, 4em)");
        var (root, _, _) = MakeTree(style, MakeStyle());

        DeferredLengthResolver.ResolveTreeInPlace(root, PageW, PageH);

        Assert.Equal(100.0, style.ReadLengthPxOrZero(PropertyId.Width), 3); // √(60² + 80²)
    }
}
