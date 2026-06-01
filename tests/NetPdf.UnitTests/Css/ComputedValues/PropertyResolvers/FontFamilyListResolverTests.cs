// Copyright 2026 Roland Aroche and NetPdf contributors.
// Licensed under the Apache License, Version 2.0. See LICENSE in the repository root.

using NetPdf.Css.ComputedValues;
using NetPdf.Css.ComputedValues.PropertyResolvers;
using NetPdf.Css.Properties;
using Xunit;

namespace NetPdf.UnitTests.Css.ComputedValues.PropertyResolvers;

/// <summary>
/// Phase 5 layout→PDF cycle 4 — unit tests for <see cref="FontFamilyListResolver"/>
/// (CSS Fonts 4 §2.1). Parses the comma-separated family list into a side-table
/// <see cref="FontFamilyList"/>; <see cref="FontReaders.ReadFontFamily"/> decodes it.
/// </summary>
public sealed class FontFamilyListResolverTests
{
    private static FontFamilyList Read(string value)
    {
        var result = PropertyResolverDispatch.Resolve(PropertyId.FontFamily, value);
        var style = ComputedStyle.RentForExclusiveTesting();
        result.MaterializeInto(style, PropertyId.FontFamily);
        return style.ReadFontFamily();
    }

    [Fact]
    public void Parses_a_comma_separated_list_in_order()
    {
        var list = Read("Arial, sans-serif");
        Assert.Equal(new[] { "Arial", "sans-serif" }, list.Families.ToArray());
        Assert.Equal("Arial", list.Primary);
    }

    [Fact]
    public void Strips_quotes_and_preserves_multi_word_names()
    {
        Assert.Equal(new[] { "Times New Roman", "serif" }, Read("\"Times New Roman\", serif").Families.ToArray());
        Assert.Equal(new[] { "Foo Bar" }, Read("'Foo Bar'").Families.ToArray());       // single-quoted
        Assert.Equal(new[] { "Courier New" }, Read("Courier  New").Families.ToArray()); // unquoted, ws-collapsed
    }

    [Fact]
    public void Single_generic_resolves()
    {
        Assert.Equal(new[] { "monospace" }, Read("monospace").Families.ToArray());
    }

    [Fact]
    public void Unset_reads_the_initial_serif_default()
    {
        var style = ComputedStyle.RentForExclusiveTesting();
        Assert.Equal(new[] { "serif" }, style.ReadFontFamily().Families.ToArray());
    }

    [Fact]
    public void Resolves_into_the_side_table()
    {
        var result = PropertyResolverDispatch.Resolve(PropertyId.FontFamily, "Arial, sans-serif");
        Assert.True(result.IsResolved);
        Assert.Equal(ComputedSlotTag.SideTableIndex, result.Slot.Tag);
    }
}
