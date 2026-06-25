// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.Properties;
using NetPdf.Layout.Boxes;
using NetPdf.Layout.Layouters;
using Xunit;

namespace NetPdf.UnitTests.Phase3;

/// <summary>
/// multicol-balancing-pagination (font-relative resolution) — unit tests for the
/// font-relative <c>column-width</c> + <c>column-gap</c> resolution path. CSS Multi-column
/// L1 §3.1's introductory example is <c>column-width: 12em</c>; the cascade DEFERS em / rem
/// (the typed slot stays <see cref="ComputedSlotTag.Unset"/> while the raw rides along), so
/// these used lengths only become available once a box's font-size is known.
///
/// <para><b>column-width</b> is resolved in place by <see cref="DeferredLengthResolver"/>
/// (mirroring <c>width</c> / <c>height</c>) BEFORE layout, so the multicol dispatch
/// (<c>BlockLayouter.IsMulticolContainer</c> → <c>ReadColumnWidth</c>) sees a resolved
/// <see cref="ComputedSlotTag.LengthPx"/> slot.</para>
///
/// <para><b>column-gap</b> is resolved locally in
/// <see cref="ComputedStyleLayoutExtensions.ReadColumnGap"/> instead, because its
/// <c>normal</c> initial is itself font-relative (1em — a keyword, not a deferred raw) AND
/// the property is shared with the flex / grid gutter, where <c>normal</c> computes to 0.</para>
/// </summary>
public sealed class MulticolFontRelativeLengthTests
{
    // ====================================================================
    //  column-width — resolved by DeferredLengthResolver (Task 1)
    // ====================================================================

    [Fact]
    public void Deferred_em_column_width_resolves_against_the_owning_box_font_size()
    {
        // Root (synthetic) → A (font-size 16) → B (font-size 20, `column-width: 12em`).
        // `em` scales by the OWNING box's font-size, so B's 12em = 12 × 20 = 240 px
        // (NOT 12 × A's 16 = 192 — the test proves the OWN font-size is the em base).
        var root = Box.CreateRoot(MakeStyle());

        var aStyle = MakeStyle();
        aStyle.Set(PropertyId.FontSize, ComputedSlot.FromLengthPx(16));
        var a = Box.ForElement(BoxKind.BlockContainer, aStyle, MakeElement());
        root.AppendChild(a);

        var bStyle = MakeStyle();
        bStyle.Set(PropertyId.FontSize, ComputedSlot.FromLengthPx(20));
        bStyle.SetDeferred(PropertyId.ColumnWidth, "12em");
        var b = Box.ForElement(BoxKind.BlockContainer, bStyle, MakeElement());
        a.AppendChild(b);

        // Pre-resolution: the slot is deferred, so ReadColumnWidth sees no LengthPx.
        Assert.True(bStyle.IsDeferred(PropertyId.ColumnWidth));
        Assert.Null(bStyle.ReadColumnWidth());

        DeferredLengthResolver.ResolveTreeInPlace(root, pageWidthPx: 600, pageHeightPx: 800);

        Assert.Equal(240, bStyle.ReadColumnWidth());
        Assert.False(bStyle.IsDeferred(PropertyId.ColumnWidth));
    }

    [Fact]
    public void Deferred_rem_column_width_resolves_against_the_root_font_size()
    {
        // Root (synthetic) → A (font-size 16 = the root ELEMENT) → B (font-size 50,
        // `column-width: 4rem`). `rem` scales by the ROOT element's font-size, so B's
        // 4rem = 4 × 16 = 64 px (NOT 4 × B's own 50 = 200 — proves the rem base is the root).
        var root = Box.CreateRoot(MakeStyle());

        var aStyle = MakeStyle();
        aStyle.Set(PropertyId.FontSize, ComputedSlot.FromLengthPx(16));
        var a = Box.ForElement(BoxKind.BlockContainer, aStyle, MakeElement());
        root.AppendChild(a);

        var bStyle = MakeStyle();
        bStyle.Set(PropertyId.FontSize, ComputedSlot.FromLengthPx(50));
        bStyle.SetDeferred(PropertyId.ColumnWidth, "4rem");
        var b = Box.ForElement(BoxKind.BlockContainer, bStyle, MakeElement());
        a.AppendChild(b);

        DeferredLengthResolver.ResolveTreeInPlace(root, pageWidthPx: 600, pageHeightPx: 800);

        Assert.Equal(64, bStyle.ReadColumnWidth());
    }

    [Fact]
    public void Absolute_and_auto_column_width_are_untouched_by_the_resolver()
    {
        // Byte-identity guard: the resolver only rewrites DEFERRED slots, so an absolute
        // `column-width: 100px` (LengthPx) and an `auto` (unset) column-width are no-ops —
        // exactly why adding column-width to the resolver's set is byte-identical for every
        // existing document (none of which carries a font-relative column-width).
        var root = Box.CreateRoot(MakeStyle());

        var pxStyle = MakeStyle();
        pxStyle.Set(PropertyId.FontSize, ComputedSlot.FromLengthPx(20));
        pxStyle.Set(PropertyId.ColumnWidth, ComputedSlot.FromLengthPx(100));
        var pxBox = Box.ForElement(BoxKind.BlockContainer, pxStyle, MakeElement());
        root.AppendChild(pxBox);

        var autoStyle = MakeStyle();
        autoStyle.Set(PropertyId.FontSize, ComputedSlot.FromLengthPx(20));
        var autoBox = Box.ForElement(BoxKind.BlockContainer, autoStyle, MakeElement());
        root.AppendChild(autoBox);

        DeferredLengthResolver.ResolveTreeInPlace(root, pageWidthPx: 600, pageHeightPx: 800);

        Assert.Equal(100, pxStyle.ReadColumnWidth());
        Assert.Null(autoStyle.ReadColumnWidth());
    }

    // ====================================================================
    //  Helpers (mirror MulticolLayouterTests)
    // ====================================================================

    private static ComputedStyle MakeStyle() => ComputedStyle.RentForExclusiveTesting();

    private static AngleSharp.Dom.IElement MakeElement()
    {
        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var doc = parser.ParseDocument("<div></div>");
        return doc.CreateElement("div");
    }
}
